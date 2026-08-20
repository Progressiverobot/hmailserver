alter table hm_domains add column domainrelayhost varchar(255) not null default '';
alter table hm_domains add column domainrelayport int not null default 0;
alter table hm_domains add column domainrelayrequiresauth int not null default 0;
alter table hm_domains add column domainrelayusername varchar(255) not null default '';
alter table hm_domains add column domainrelaypassword varchar(255) not null default '';
alter table hm_domains add column domainrelayconnectionsecurity int not null default 0;

update hm_dbversion set value = 6021;
