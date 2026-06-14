alter table hm_routes modify routeauthenticationpassword varchar(1024) NOT NULL;

alter table hm_fetchaccounts modify fapassword varchar(1024) not null;

update hm_dbversion set value = 6004;
