alter table hm_messages add column messageemailid varchar(48) not null default '';

update hm_messages set messageemailid = 'M0' || cast(messageid as varchar);

update hm_dbversion set value = 6015;
