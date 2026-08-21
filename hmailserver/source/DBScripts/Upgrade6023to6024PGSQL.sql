alter table hm_domains add column domainvacationmessageon int not null default 0;

alter table hm_domains add column domainvacationsubject varchar(200) not null default '';

alter table hm_domains add column domainvacationmessage text not null default '';

alter table hm_domains add column domainvacationinternalsubject varchar(200) not null default '';

alter table hm_domains add column domainvacationinternalmessage text not null default '';

alter table hm_domains add column domainvacationexternaloverride int not null default 0;

create table hm_blocked_senders
(
	bsid bigserial not null primary key,
	bsaddress varchar(255) not null,
	bsscore int not null,
	bsdescription varchar(255) not null,
	constraint u_bsaddress unique (bsaddress)
);

update hm_dbversion set value = 6024;
